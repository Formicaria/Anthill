using Anthill.SDK.Security;
using Anthill.SDK.Tools;

namespace Anthill.SDK.Common;

/// <summary>
/// The settings <see cref="UrlSafety"/>, <see cref="Validation"/> and
/// <see cref="ToolDefinition.Validate"/> fall back to when no options argument is passed. v3.8.12,
/// extended in v3.8.15.
///
/// WHY THIS EXISTS AT ALL. Both helpers are static, and all 21 call sites across the core call them
/// statically. Converting them to instance types would have rewritten every one of those call sites
/// and forced <c>Queen</c>, <c>SelfTest</c> and <c>PheromoneEngine</c> to hold options objects they
/// have no other use for. So the impure methods take an OPTIONAL options argument instead, and this
/// is what <c>null</c> resolves to.
///
/// WHY THE DEFAULT IS SETTABLE RATHER THAN BAKED IN. The values these guards read are mutable
/// statics on <c>AnthillRuntime</c>, and the test suite writes to them — <c>SecurityTests</c> and
/// <c>HomelabFoundationTests</c> both depend on a blocked host or path being observed by a later
/// call. A hard-coded copy in the SDK would have quietly stopped tracking those writes, which is
/// precisely the failure v3.8.11 was written to avoid: a test that passes while the production path
/// reads something else.
///
/// WHY THE BUILT-IN FALLBACKS ARE STILL SAFE. Until a composition root calls <see cref="Configure"/>,
/// these return the same values <c>AnthillRuntime</c> declares as ITS defaults. An unconfigured
/// process is therefore never more permissive than a configured one — it just cannot see subsequent
/// operator edits. <c>Anthill.Core</c> installs the live readers from a module initializer, so any
/// process that loads the core is configured before its first line of colony code runs.
/// </summary>
public static class SafetyPolicy
{
    private static readonly object Gate = new();
    private static ISsrfPolicy _ssrf = new BuiltInSsrfPolicy();
    private static IToolRuntimeOptions? _toolOptions;
    private static IToolDefinitionPolicy _toolDefinitions = new BuiltInToolDefinitionPolicy();

    /// <summary>The outbound blocklist <see cref="UrlSafety.IsBlockedOutboundUrl"/> uses by default.</summary>
    public static ISsrfPolicy Ssrf
    {
        get { lock (Gate) return _ssrf; }
    }

    /// <summary>
    /// The patch-path gates <see cref="Validation.ValidateSafePatchPath"/> uses by default.
    /// Null until a composition root supplies one; the validator then falls back to its own
    /// built-in gates, which mirror the core's declared defaults.
    /// </summary>
    public static IToolRuntimeOptions? ToolOptions
    {
        get { lock (Gate) return _toolOptions; }
    }

    /// <summary>
    /// What a build reserves, refuses and can construct, for <see cref="ToolDefinition.Validate"/>.
    /// v3.8.15.
    ///
    /// Never null, unlike <see cref="ToolOptions"/>. A definition validated against no policy at all
    /// would be a definition free to shadow <c>apply_patch</c>, so the unconfigured path gets the
    /// built-in mirror below rather than a permissive default.
    /// </summary>
    public static IToolDefinitionPolicy ToolDefinitions
    {
        get { lock (Gate) return _toolDefinitions; }
    }

    /// <summary>
    /// Called once at startup, before anything that validates a URL, a patch path or a tool
    /// definition is constructed. <c>Anthill.Core</c> calls this from a module initializer, so tests
    /// that never build a colony still get the live readers.
    /// </summary>
    public static void Configure(
        ISsrfPolicy? ssrf = null,
        IToolRuntimeOptions? toolOptions = null,
        IToolDefinitionPolicy? toolDefinitions = null)
    {
        lock (Gate)
        {
            if (ssrf is not null) _ssrf = ssrf;
            if (toolOptions is not null) _toolOptions = toolOptions;
            if (toolDefinitions is not null) _toolDefinitions = toolDefinitions;
        }
    }

    /// <summary>Restores the built-in fallbacks. For tests that need to prove the unconfigured path.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _ssrf = new BuiltInSsrfPolicy();
            _toolOptions = null;
            _toolDefinitions = new BuiltInToolDefinitionPolicy();
        }
    }

    private sealed class BuiltInSsrfPolicy : ISsrfPolicy
    {
        // Mirrors AnthillRuntime.SsrfBlockedHostnames / SsrfBlockedHostSuffixes as declared.
        public IReadOnlySet<string> BlockedHostnames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };

        public IReadOnlyList<string> BlockedHostSuffixes { get; } = new[] { ".localhost", ".local" };
    }

    /// <summary>
    /// Mirrors <c>ToolInventory.Implemented</c>, <c>ToolAuthorization.MissionAgentForbidden</c> and
    /// the kinds <c>UserToolRegistrar.Default()</c> constructs, as the core declares them today.
    ///
    /// A duplicated list is a drift hazard and this one is deliberate, for the same reason
    /// <see cref="BuiltInSsrfPolicy"/> is: the alternative to mirroring is an unconfigured process
    /// with an EMPTY reserved-name set, which is not a smaller failure than drift — it is a process
    /// in which a definition may take a built-in's name. Mirroring makes the unconfigured path
    /// strictly no more permissive than the configured one; it merely stops tracking later edits.
    ///
    /// The drift is closed by a test rather than by discipline: <c>ToolDefinitionPolicyTests</c>
    /// asserts this set equals the core's live tables, so ADDING A TOOL to the inventory and
    /// forgetting this list fails the build rather than quietly widening what a definition may
    /// shadow in a process that never loaded the core.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <c>ToolDefinitionPolicyTests</c> can compare it against the
    /// core's tables directly. The alternative — reading it back through
    /// <see cref="Reset"/> — would have the pinning test mutate process-wide safety state to
    /// inspect it, which is a strange thing for a test of safety state to do.
    /// </remarks>
    internal sealed class BuiltInToolDefinitionPolicy : IToolDefinitionPolicy
    {
        public IReadOnlySet<string> ReservedToolNames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system_info",
                "run_allowlisted_check",
                "list_directory",
                "read_text_file",
                "write_text_file",
                "web_search",
                "shell_command",
                "apply_patch",
                "search_workspace",
                "read_changed_files_summary",
                "repository_index",
            };

        public IReadOnlySet<string> ForbiddenToolNames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "apply_patch", "shell_command", "write_text_file",
            };

        public IReadOnlySet<ToolKind> BuildableKinds { get; } = new HashSet<ToolKind> { ToolKind.Http };
    }
}
