using System.Text.Json.Serialization;

namespace Anthill.SDK.Contracts;

// v3.8.9 — the HALF of the old Anthill.Core.Contracts.TaskContracts that is genuinely shared
// vocabulary: what a capability is, how a failure is classified, and what a tool declares about
// itself. Nothing here knows what a mission or a task is.
//
// The other half stayed in the core, and the reason is the whole lesson of this split. An earlier
// attempt moved the entire file after checking its `using` statements and finding none — but
// `TaskContract.FromTask` takes `Domain.Task` and reaches `Agents.AntRegistry`, and `ContractGate`
// takes `List<Domain.Task>`, all through PARTIAL qualification that resolved via the enclosing
// namespace and left no import to notice. Those three types are core planning logic that happened
// to share a file with five pure ones.
//
// `ToolResult` also stayed, deliberately: Anthill.Core.Domain declares a DIFFERENT ToolResult, and
// call sites disambiguate with `Contracts.ToolResult`. Moving it would have broken every one of
// them in a way that reads as an unrelated ambiguity error.

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
