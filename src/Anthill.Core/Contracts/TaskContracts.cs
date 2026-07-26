using System.Text.Json.Serialization;

namespace Anthill.Core.Contracts;

/// <summary>
/// v2.9.0 — Contracted Tasks and Typed Capability Tools (NORTH_STAR V3-track Phase 2).
/// Machine-readable contracts replace loose prompt tasks and string-parsed tool results as the
/// control-flow surface: planner output is schema-validated (invalid tasks cannot enter the
/// execution queue), permissions attach to CAPABILITIES rather than ant names and are evaluable
/// before execution, and failures are classified by a fixed taxonomy that drives retry decisions.
/// </summary>
public static class Capability
{
    public const string RepoRead = "repo.read";
    public const string RepoSearch = "repo.search";
    public const string RepoWriteSandbox = "repo.write.sandbox";
    public const string RepoPatchPropose = "repo.patch.propose";
    public const string RepoPatchApply = "repo.patch.apply";
    public const string ProcessExecuteReadonly = "process.execute.readonly";
    public const string NetworkHttpPublic = "network.http.public";
    public const string NetworkHttpHomelab = "network.http.homelab";
    public const string ModelInvoke = "model.invoke";
    public const string ProxmoxRead = "proxmox.read";
    public const string ProxmoxVmStart = "proxmox.vm.start";
    public const string ProxmoxVmStop = "proxmox.vm.stop";
    public const string ProxmoxSnapshotCreate = "proxmox.snapshot.create";
    public const string CredentialUse = "credential.use";
}

/// <summary>The fixed failure taxonomy. Retry decisions come from the class, never from parsing
/// error strings.</summary>
public enum FailureClass
{
    None = 0,
    ValidationFailure, AuthorizationFailure, TargetRejection,
    TransientProviderFailure, RateLimit, Timeout, Conflict,
    DependencyFailure, VerificationFailure, UnsafeState,
    CompensationFailure, InternalDefect,
}

public static class FailureClassify
{
    /// <summary>Only these classes may be retried automatically; everything else needs a human
    /// or a plan change. Unknown fails toward NOT retryable.</summary>
    public static bool IsRetryable(FailureClass c) => c is FailureClass.TransientProviderFailure
        or FailureClass.RateLimit or FailureClass.Timeout or FailureClass.Conflict;
}

/// <summary>Typed declaration of one tool/caste: what it can touch, what it needs, how it fails.</summary>
public sealed class ToolDescriptor
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "1";
    [JsonPropertyName("required_capabilities")] public string[] RequiredCapabilities { get; init; } = Array.Empty<string>();
    [JsonPropertyName("side_effect_class")] public string SideEffectClass { get; init; } = "none"; // none | reversible | destructive
    [JsonPropertyName("risk_class")] public string RiskClass { get; init; } = "low"; // low | medium | high | critical
    [JsonPropertyName("idempotent")] public bool Idempotent { get; init; }
    [JsonPropertyName("supports_cancellation")] public bool SupportsCancellation { get; init; } = true;
    [JsonPropertyName("supports_timeout")] public bool SupportsTimeout { get; init; } = true;
    [JsonPropertyName("compensation")] public string Compensation { get; init; } = "none"; // none | manual | automatic
}

/// <summary>
/// The typed tool catalog for today's executable castes. Honest declarations of what EXISTS —
/// no capability is granted here; this is what each caste WOULD need, evaluable pre-execution.
/// </summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyDictionary<string, ToolDescriptor> Tools =
        new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase)
    {
        ["researcher"] = new() { Name = "researcher", Description = "Model-only analysis and synthesis.", RequiredCapabilities = new[] { Capability.ModelInvoke }, SideEffectClass = "none", RiskClass = "low", Idempotent = true },
        ["web"] = new() { Name = "web", Description = "Public web search/fetch.", RequiredCapabilities = new[] { Capability.ModelInvoke, Capability.NetworkHttpPublic }, SideEffectClass = "none", RiskClass = "low", Idempotent = true },
        ["file"] = new() { Name = "file", Description = "Read-only workspace inspection.", RequiredCapabilities = new[] { Capability.RepoRead, Capability.RepoSearch }, SideEffectClass = "none", RiskClass = "low", Idempotent = true },
        ["coder"] = new() { Name = "coder", Description = "Patch proposals (apply is separately gated).", RequiredCapabilities = new[] { Capability.ModelInvoke, Capability.RepoRead, Capability.RepoPatchPropose }, SideEffectClass = "reversible", RiskClass = "medium", Idempotent = false, Compensation = "manual" },
        ["builder"] = new() { Name = "builder", Description = "Build/assemble outputs in the sandbox.", RequiredCapabilities = new[] { Capability.ModelInvoke, Capability.RepoWriteSandbox }, SideEffectClass = "reversible", RiskClass = "medium", Idempotent = false, Compensation = "manual" },
        ["verifier"] = new() { Name = "verifier", Description = "Independent result verification.", RequiredCapabilities = new[] { Capability.ModelInvoke, Capability.RepoRead }, SideEffectClass = "none", RiskClass = "low", Idempotent = true },
    };

    public static ToolDescriptor? Describe(string ant) => Tools.TryGetValue(ant ?? "", out var d) ? d : null;

    /// <summary>Pre-execution permission check: does the grant set cover the tool's needs?
    /// Unknown tools fail toward refusal.</summary>
    public static bool CanRun(string ant, IReadOnlyCollection<string> grantedCapabilities)
    {
        var d = Describe(ant);
        return d is not null && d.RequiredCapabilities.All(grantedCapabilities.Contains);
    }
}

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
