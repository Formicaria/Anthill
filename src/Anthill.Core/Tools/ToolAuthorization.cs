using Anthill.Core.Agents;

namespace Anthill.Core.Tools;

/// <summary>
/// Ant Execution Framework — Stage B: capability enforcement at tool dispatch. Until now the
/// ant identity passed to <c>RunTool</c> was a free string used only for logging: ANY caller could
/// invoke ANY registered tool (including shell_command and apply_patch) under any name. This class
/// makes the declared boundaries enforceable at the dispatch chokepoint, fail closed:
///  - unknown non-empty ant names are DENIED (spoofing a name grants nothing),
///  - mission agents run only their role's dispatch allowlist,
///  - apply_patch / shell_command / write_text_file are structurally forbidden to every mission
///    agent — patch application stays inside the queen/director approval pipeline,
///  - denials return a structured failure, run nothing, and land on the audit stream.
/// System-internal calls (null/empty ant name) keep compatibility and remain audited.
/// </summary>
public sealed record ToolExecutionContext(
    string MissionId,
    string TaskId,
    string RoleId,
    string WorkerId,
    IReadOnlySet<string> GrantedCapabilities,
    IReadOnlySet<string> AllowedTools,
    IReadOnlySet<string> ForbiddenTools);

public static class ToolAuthorization
{
    /// <summary>Tools no mission agent may ever dispatch, regardless of any declared allowlist.</summary>
    public static readonly IReadOnlySet<string> MissionAgentForbidden =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apply_patch", "shell_command", "write_text_file" };

    /// <summary>Control-plane callers: their tool calls ARE the orchestration/approval pipeline
    /// (e.g. queen/director invoking apply_patch after human approval). Audited, not blocked here.</summary>
    private static readonly IReadOnlySet<string> ControlPlane =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "queen", "director" };

    /// <summary>Per-role dispatch allowlists for the executable mission agents, derived from their
    /// real call sites and read-only duties. A role absent here (coder/builder/verifier are
    /// model-only today) has an EMPTY allowlist — fail closed.</summary>
    private static readonly Dictionary<string, HashSet<string>> RoleAllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["researcher"] = new(StringComparer.OrdinalIgnoreCase) { "system_info", "list_directory" },
        ["web"] = new(StringComparer.OrdinalIgnoreCase) { "web_search", "system_info" },
        ["file"] = new(StringComparer.OrdinalIgnoreCase) { "list_directory", "read_text_file", "system_info" },
        ["coder"] = new(StringComparer.OrdinalIgnoreCase),
        ["builder"] = new(StringComparer.OrdinalIgnoreCase),
        ["verifier"] = new(StringComparer.OrdinalIgnoreCase),
    };

    public sealed record Decision(bool Allowed, string Reason)
    {
        public static readonly Decision Ok = new(true, "");
        public static Decision Deny(string reason) => new(false, reason);
    }

    /// <summary>Authorization for the legacy string-identity dispatch path. Stage D moves
    /// specialist execution to full <see cref="ToolExecutionContext"/> evaluation.</summary>
    public static Decision Evaluate(string? antName, string toolName)
    {
        if (string.IsNullOrWhiteSpace(antName)) return Decision.Ok; // system-internal (audited upstream)
        var role = antName.Trim();

        if (ControlPlane.Contains(role)) return Decision.Ok;

        // Specialist contract roles (Stage A): enforce their declared contract even before
        // activation, so a prematurely wired handler still cannot exceed its contract.
        var contract = AntExecutionCatalog.ContractFor(role);
        if (contract is not null)
        {
            if (MissionAgentForbidden.Contains(toolName) || contract.ForbiddenTools.Contains(toolName))
                return Decision.Deny($"tool '{toolName}' is forbidden to role '{role}' by its execution contract");
            if (!contract.AllowedTools.Contains(toolName))
                return Decision.Deny($"tool '{toolName}' is not in role '{role}' contract allowlist");
            return Decision.Ok;
        }

        if (RoleAllowedTools.TryGetValue(role, out var allowed))
        {
            if (MissionAgentForbidden.Contains(toolName))
                return Decision.Deny($"tool '{toolName}' is structurally forbidden to mission agents (role '{role}')");
            if (!allowed.Contains(toolName))
                return Decision.Deny($"tool '{toolName}' is not in role '{role}' dispatch allowlist");
            return Decision.Ok;
        }

        // Unknown non-empty identity: spoofing a name must never widen access.
        return Decision.Deny($"unknown ant identity '{role}' — tool dispatch refused (fail closed)");
    }

    /// <summary>Full-context authorization used by Stage C/D runtime execution.</summary>
    public static Decision Evaluate(ToolExecutionContext ctx, string toolName)
    {
        if (MissionAgentForbidden.Contains(toolName))
            return Decision.Deny($"tool '{toolName}' is structurally forbidden to mission agents");
        if (ctx.ForbiddenTools.Contains(toolName))
            return Decision.Deny($"tool '{toolName}' is forbidden to role '{ctx.RoleId}'");
        if (!ctx.AllowedTools.Contains(toolName))
            return Decision.Deny($"tool '{toolName}' is not allowlisted for role '{ctx.RoleId}'");
        var contract = AntExecutionCatalog.ContractFor(ctx.RoleId);
        if (contract is not null && !contract.RequiredCapabilities.All(ctx.GrantedCapabilities.Contains))
            return Decision.Deny($"role '{ctx.RoleId}' is missing required capabilities for dispatch");
        return Decision.Ok;
    }
}
