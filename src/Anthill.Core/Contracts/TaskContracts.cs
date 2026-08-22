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

    /// <summary>
    /// Whether <see cref="RequiredCapabilities"/> came from a KNOWN role contract. v0.3.8.87.
    ///
    /// Not decoration — <see cref="Validate"/> reads it, and the distinction it carries is the whole
    /// reason the archivist used to be described as invoking a model.
    ///
    /// An empty capability list has two meanings and the schema could express only one. "This role
    /// requires nothing" is a real, correct declaration — <c>AntExecutionCatalog</c> makes it for the
    /// archivist, which consolidates memory the Queen hands it and touches nothing else. "We do not
    /// know what this role requires" is the unknown-ant case and must stay a rejection. Before this
    /// flag both arrived as `Count == 0`, so the guard could only reject both — and the projection
    /// dodged it by declaring <c>model.invoke</c> for six roles, five of which hold no ModelRouter at
    /// all (v0.3.8.76). A guard that cannot express "requires nothing" makes every honest caller lie
    /// to it.
    /// </summary>
    [JsonPropertyName("capabilities_declared_by_contract")]
    public bool CapabilitiesDeclaredByContract { get; set; }

    /// <summary>
    /// Project a planner task into its contract. ONE BOOK — v0.3.8.87.
    ///
    /// Every field below that describes the ROLE now comes from <c>AntExecutionCatalog</c>, the same
    /// declaration <c>ToolAuthorization.Evaluate</c> enforces at dispatch. Until this release they
    /// came from <c>ToolCatalog</c>, which nothing enforced and which disagreed with the contracts
    /// about capabilities for four roles, about side effects for two, and about six roles it did not
    /// list at all. This gate decides ADMISSION; that one decides DISPATCH; a task could be admitted
    /// on one declaration and refused on the other. The note at the bottom of ToolVocabulary.cs has
    /// the full list of what the two books disagreed about.
    ///
    /// SIDE EFFECTS ARE DERIVED, not restated. Every contract today declares
    /// <c>AllowsSideEffects: false</c> — including the coder, which PROPOSES patches and never
    /// applies them — so deriving the class from that flag produces "none" where the old catalog
    /// said "reversible". The flag is the authority: it is what the runtime reads, and a projection
    /// that contradicted it was describing a colony this one is not.
    ///
    /// Risk follows patch proposals rather than side effects, because a proposal is the thing an
    /// operator must review even though it changes nothing by itself.
    /// </summary>
    public static TaskContract FromTask(Domain.Task t)
    {
        var role = t.AssignedAnt ?? "";
        var contract = Agents.AntExecutionCatalog.ContractFor(role);

        // A role the registry says is executable+enabled but that has no contract must not be
        // silently un-plannable — it gets a cautious declaration instead. Ants unknown to BOTH stay
        // capability-less and are rejected, which is the behaviour this has always had.
        var executableWithoutContract =
            contract is null && Agents.AntRegistry.ExecutableRoleIds.Contains(role);

        var sideEffect = contract is not null
            ? (contract.AllowsSideEffects ? "reversible" : "none")
            : executableWithoutContract ? "reversible" : "destructive"; // unknown ant fails toward caution

        var risk = contract is not null
            ? (contract.ProducesPatchProposals ? "medium" : "low")
            : executableWithoutContract ? "high" : "critical";

        return new TaskContract
        {
            Id = t.Id, Title = t.Title, Objective = t.Description,
            TaskType = t.TaskType switch
            {
                "verification" => "verify",
                "research" or "analysis" => "research",
                "patch_proposal" or "patch" or "code_change" or "build" => "change",
                _ => sideEffect == "none" ? "diagnose" : "change",
            },
            RequiredCapabilities = contract?.RequiredCapabilities.ToList() ?? new List<string>(),
            CapabilitiesDeclaredByContract = contract is not null,
            SideEffectClass = sideEffect,
            RiskClass = risk,
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
        // SPLIT, not softened — v0.3.8.87. The permanent half is unchanged and still fails closed: a
        // role no contract describes cannot be permission-checked, so its task stays out of the
        // queue. What was wrong was the inference that empty MEANS unknown. A contract that declares
        // zero capabilities has answered the question; it has not declined to.
        //
        // The flag can only be set by a lookup that SUCCEEDED, so this cannot be widened by an
        // absent role: an unknown ant leaves it false and lands on the same rejection it always did.
        if (RequiredCapabilities.Count == 0 && !CapabilitiesDeclaredByContract)
            errors.Add("no role contract declares this ant's capabilities, so the task cannot be "
                     + "permission-checked before dispatch");
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

