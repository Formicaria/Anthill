using System.Text.Json;
using System.Text.Json.Nodes;
using Anthill.SDK.Common;
using Anthill.SDK.Contracts;

namespace Anthill.SDK.Tools;

// v3.8.15 — phase 5c step 3. Moved out of Anthill.Core.Tools so a module can declare a tool KIND
// and not merely an instance: IToolKindExecutor names ToolDefinition in its signature, so neither
// could move without the other.
//
// The move cost exactly three lines of coupling, and all three were in Validate(): the built-in
// name table, the structurally-forbidden name table, and the buildable kind set. All three describe
// what the CORE registers, so none of them followed the record here. They arrive through
// IToolDefinitionPolicy instead, resolved the same way UrlSafety and Validation resolve theirs.
//
// The file's original alias for Anthill.Core.Contracts.ToolResult is gone with it: inside the SDK
// there is only one ToolResult, and the colliding contract-shaped one stays in the core.

/// <summary>
/// v3.4.1 (ADR-006) — a tool the OPERATOR defined, as data.
///
/// Every tool up to now has been a C# class compiled into the build, which makes the tool ecosystem
/// exactly as extensible as the release cycle. This is the other half: a tool described by a record
/// an operator can write, store, inspect and revoke, without a rebuild.
///
/// DATA, NOT CODE, and that is the whole safety argument. A definition cannot express "run this" —
/// it names a <see cref="ToolKind"/> and supplies configuration that the kind's executor interprets
/// under its own rules and its own config gate. So the blast radius of a bad definition is bounded
/// by the kind it names, and adding a kind is a reviewed change to this repository rather than
/// something a definition can do to itself.
///
/// The consequence worth stating plainly: a model that can register tools cannot thereby grant
/// itself new powers. It can only recombine powers a human already built and switched on.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>
    /// The name a model calls. Must not collide with a built-in — see <see cref="Validate"/>, where
    /// shadowing is refused rather than resolved, because a definition that could take over
    /// <c>apply_patch</c> would turn tool registration into privilege escalation.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// What it does, in the model's terms. Required and non-trivial: this is the ONLY thing a model
    /// reads when deciding whether to call it, so an empty description ships a tool nothing will
    /// ever use, which looks identical to a tool that does not work.
    /// </summary>
    public required string Description { get; init; }

    public required ToolKind Kind { get; init; }

    /// <summary>
    /// JSON Schema for the arguments, handed to the provider verbatim — the same contract
    /// <see cref="ITool.ParametersJson"/> states for built-ins, so the projection needs no special
    /// case for user tools.
    /// </summary>
    public string ParametersJson { get; init; } = """{"type":"object","properties":{}}""";

    /// <summary>Kind-specific configuration. Interpreted ONLY by that kind's executor.</summary>
    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Roles permitted to dispatch it. EMPTY MEANS EVERY DISPATCHING ROLE — the operator's stated
    /// intent is that any role which can use a tool may use it, and a user tool that nobody can call
    /// is a feature that does not work.
    ///
    /// This is deliberately more permissive than the built-in allowlists, and the reason it is safe
    /// to be: the built-in lists exist to keep <c>apply_patch</c> and <c>shell_command</c> away from
    /// mission agents, and the core's <c>ToolAuthorization.MissionAgentForbidden</c> still applies
    /// by name regardless of what any definition says.
    /// </summary>
    public IReadOnlyList<string> AllowedRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Disabled definitions are KEPT, not deleted. Revoking a tool and forgetting it ever existed
    /// are different operator intents, and an audit that cannot see a withdrawn tool cannot explain
    /// a transcript in which a model called it.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public string CreatedBy { get; init; } = "operator";
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();

    /// <summary>
    /// Whether <paramref name="role"/> may dispatch this. The grant list only WIDENS from empty;
    /// it can never override the structural prohibitions, which are enforced separately.
    /// </summary>
    public bool GrantsRole(string? role) =>
        AllowedRoles.Count == 0 ||
        AllowedRoles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reject a definition that cannot work, at REGISTRATION rather than at call time.
    ///
    /// This is the whole reason validation is a separate step: a malformed definition that is
    /// accepted becomes a tool offered to a model, called, and failed — once per turn, burning
    /// budget, with the real fault three layers away from the symptom. Refusing it at the door costs
    /// the operator one error message.
    /// </summary>
    /// <param name="policy">
    /// What this build reserves, refuses and can construct. v3.8.15 — three of the checks below are
    /// facts about the CORE, and the core is what answers them. <c>null</c> resolves through
    /// <see cref="SafetyPolicy.ToolDefinitions"/>, which <c>Anthill.Core</c> installs from a module
    /// initializer; the argument exists so a test can pin a policy without touching global state.
    ///
    /// Deliberately optional rather than required. Every caller in the tree passes nothing, and
    /// making it required would have rewritten them all — including the security tests that assert
    /// on this method directly — to thread through an object whose only correct value is the one
    /// the core already installed.
    /// </param>
    public IReadOnlyList<string> Validate(IToolDefinitionPolicy? policy = null)
    {
        var rules = policy ?? SafetyPolicy.ToolDefinitions;
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("name is required");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(Name, "^[a-z][a-z0-9_]{2,63}$"))
            problems.Add($"name '{Name}' must be 3-64 chars, lowercase, starting with a letter "
                       + "(providers reject tool names outside this shape)");
        else if (rules.ReservedToolNames.Contains(Name))
            problems.Add($"'{Name}' is a built-in tool — a definition may not shadow one, because a "
                       + "tool that could take over apply_patch would make registration an escalation");

        if (string.IsNullOrWhiteSpace(Description))
            problems.Add("description is required — it is the only thing a model reads when deciding to call it");

        if (Kind == ToolKind.Unknown)
            problems.Add("kind is required");
        else if (!rules.BuildableKinds.Contains(Kind))
            problems.Add($"kind '{Kind}' is declared but not built in this release; "
                       + $"available now: {string.Join(", ", rules.BuildableKinds)}");

        // The schema is PARSED, not merely stored. An unparseable schema reaches the provider as a
        // malformed tools array, which most backends answer by ignoring the tool silently — the
        // failure mode that looks exactly like a model choosing not to call it.
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(ParametersJson)
                ? """{"type":"object","properties":{}}""" : ParametersJson);
            if (node is not JsonObject) problems.Add("parameters must be a JSON Schema OBJECT");
        }
        catch (JsonException error)
        {
            problems.Add($"parameters is not valid JSON: {error.Message}");
        }

        if (rules.ForbiddenToolNames.Contains(Name))
            problems.Add($"'{Name}' is a structurally forbidden tool name");

        return problems;
    }
}

/// <summary>
/// How a user-defined tool does its work. Each kind is a REVIEWED execution path in this repository
/// with its own config gate; a definition selects among them and can never introduce one.
/// </summary>
public enum ToolKind
{
    Unknown = 0,

    /// <summary>An HTTP request to an allowlisted host. Shipped.</summary>
    Http,

    /// <summary>A named, parameterised invocation of a tool that already exists. Declared, not built.</summary>
    Composite,

    /// <summary>Tools exposed by an MCP server. Declared, not built.</summary>
    Mcp,

    /// <summary>A command from the existing SafeCommands allowlist. Declared, not built.</summary>
    Command,
}

public static class ToolKinds
{
    /// <summary>
    /// Parse a stored or wire-supplied kind name. Anything unrecognised becomes
    /// <see cref="ToolKind.Unknown"/>, which <see cref="ToolDefinition.Validate"/> then reports as
    /// "kind is required" — a definition with a typo'd kind must not silently acquire a default.
    /// </summary>
    public static ToolKind Parse(string? text) =>
        Enum.TryParse<ToolKind>(text, ignoreCase: true, out var kind) ? kind : ToolKind.Unknown;
}

/// <summary>
/// Builds the <see cref="ITool"/> for one kind. The seam that keeps kinds addable without touching
/// registration, authorization, projection or persistence — all of which are kind-agnostic and
/// should stay that way.
/// </summary>
public interface IToolKindExecutor
{
    ToolKind Kind { get; }

    /// <summary>
    /// Config problems specific to this kind, checked at registration alongside
    /// <see cref="ToolDefinition.Validate"/>. Empty means usable.
    /// </summary>
    IReadOnlyList<string> ValidateConfig(ToolDefinition definition);

    ToolResult Execute(ToolDefinition definition, IReadOnlyDictionary<string, object?> args);
}

/// <summary>
/// The <see cref="ITool"/> face of a definition. One class serves every kind, because from the
/// registry's point of view a user tool is a name, a description, a schema and something to call —
/// exactly what a built-in is.
/// </summary>
public sealed class UserDefinedTool : ITool
{
    private readonly IToolKindExecutor _executor;

    public UserDefinedTool(ToolDefinition definition, IToolKindExecutor executor)
    {
        Definition = definition;
        _executor = executor;
    }

    public ToolDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string ParametersJson => Definition.ParametersJson;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        // A disabled definition that is still registered must refuse, not run. Registration and
        // enablement are separate lifetimes: an operator revoking a tool mid-mission expects the
        // next call to fail, and expects to be able to tell that it was revoked rather than broken.
        if (!Definition.Enabled)
            return new ToolResult(Name, false, "",
                $"user-defined tool '{Name}' is disabled", FailureClass.AuthorizationFailure);

        return _executor.Execute(Definition, args);
    }
}
