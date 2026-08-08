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

/// <summary>
/// The ONE conversion between <see cref="FailureClass"/> and its string form. v3.8.32.
///
/// Before this existed the codebase had two string representations of the same enum and no agreement
/// about which was which:
///
/// <list type="bullet">
/// <item><c>TaskOutcomeMapper</c> wrote <c>transient_provider_failure</c> into <c>Task.FailureType</c>,
///   which flowed on into <c>task_attempts.failure_class</c>.</item>
/// <item><c>SqliteMemory.RecordTaskResult</c> wrote <c>TransientProviderFailure</c> into
///   <c>task_results.failure_class</c>.</item>
/// <item><c>LearningAttribution</c> compared the FIRST against the SECOND's form, with
///   <c>OrdinalIgnoreCase</c> — which bridges the casing and NOT the underscores.</item>
/// </list>
///
/// The consequence ran for six releases: the environmental-failure set matched nothing, so every
/// provider outage, rate limit, timeout, dependency failure and authorization refusal was charged as
/// a negative pheromone trail against whichever ant was holding the task. That is the precise bug
/// v3.8.26 was written to fix, and it never once worked. <c>LoadTaskResult</c>'s
/// <c>Enum.TryParse</c> had the mirror-image blind spot, and <c>WhatUsuallyFails</c> grouped the two
/// forms into separate buckets, reporting one failure class as two.
///
/// Every one of those sites had a passing test, because each test built its own input in the form
/// its own side expected. No test anywhere ran a value from a real producer into a real consumer.
///
/// So the fix is not "pick a format" — it is to remove the choice. There is one <see cref="Wire"/>
/// out and one <see cref="TryParse"/> in, <see cref="TryParse"/> normalises away the difference the
/// old code tripped on, and no caller anywhere is permitted to call <c>.ToString()</c> on a
/// <see cref="FailureClass"/> destined for storage or comparison.
/// </summary>
public static class FailureClassNames
{
    /// <summary>
    /// The canonical wire form: <c>snake_case</c>.
    ///
    /// Chosen because it is what the rest of the wire vocabulary already uses — status codes
    /// (<c>failed_retryable</c>), trail kinds (<c>model_route</c>), and the untyped failure types the
    /// runtime writes directly (<c>execution_error</c>, <c>missing_ant</c>). The PascalCase form was
    /// never a decision; it was <c>.ToString()</c> reached for at three separate call sites.
    /// </summary>
    private static readonly Dictionary<FailureClass, string> WireByClass =
        Enum.GetValues<FailureClass>().ToDictionary(c => c, c => ToSnake(c.ToString()));

    /// <summary>
    /// Lookup keyed by a NORMALISED form — lowercased with underscores removed — so both historical
    /// representations resolve to the same class.
    ///
    /// This is deliberate rather than lenient. Databases in the field already hold both forms, written
    /// by the two producers above; a parser that accepted only the new canonical form would silently
    /// drop every row written before this release, which is the same failure mode in a new coat.
    /// </summary>
    private static readonly Dictionary<string, FailureClass> ByNormalized =
        Enum.GetValues<FailureClass>().ToDictionary(c => Normalize(c.ToString()), c => c);

    /// <summary>Every canonical wire name. Ordered by the enum, so it is stable across runs.</summary>
    public static IReadOnlyCollection<string> AllWire { get; } =
        Enum.GetValues<FailureClass>().Select(c => WireByClass[c]).ToArray();

    /// <summary>The canonical string for a class. The ONLY permitted way to stringify one.</summary>
    public static string Wire(FailureClass cls) =>
        WireByClass.TryGetValue(cls, out var name) ? name : ToSnake(cls.ToString());

    /// <summary>
    /// Parse any recorded form back to the class. Accepts the canonical wire form and the legacy
    /// enum-name form; rejects anything else rather than guessing.
    /// </summary>
    /// <remarks>
    /// Returns false for the runtime's untyped failure types (<c>timeout</c> is a member, but
    /// <c>missing_ant</c>, <c>execution_error</c> and <c>blocked</c> are not). A caller must treat
    /// false as "this failure was not classified", never as "this failure was benign".
    /// </remarks>
    public static bool TryParse(string? text, out FailureClass cls)
    {
        cls = FailureClass.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ByNormalized.TryGetValue(Normalize(text), out cls);
    }

    /// <summary>Parse, or <see cref="FailureClass.None"/> when the text names no known class.</summary>
    public static FailureClass ParseOrNone(string? text) => TryParse(text, out var cls) ? cls : FailureClass.None;

    /// <summary>
    /// Casing- and separator-insensitive key. Underscores are REMOVED rather than treated as
    /// significant, which is exactly the step the old <c>OrdinalIgnoreCase</c> comparison was
    /// missing.
    /// </summary>
    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch != '_' && ch != '-' && ch != ' ') sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 6);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
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
