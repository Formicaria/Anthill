using Anthill.SDK.Security;
using Anthill.SDK.Tools;

namespace Anthill.SDK.Common;

/// <summary>
/// The settings <see cref="UrlSafety"/> and <see cref="Validation"/> fall back to when no options
/// argument is passed. v3.8.12.
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
    /// Called once at startup, before anything that validates a URL or a patch path is constructed.
    /// <c>Anthill.Core</c> calls this from a module initializer, so tests that never build a colony
    /// still get the live readers.
    /// </summary>
    public static void Configure(ISsrfPolicy? ssrf = null, IToolRuntimeOptions? toolOptions = null)
    {
        lock (Gate)
        {
            if (ssrf is not null) _ssrf = ssrf;
            if (toolOptions is not null) _toolOptions = toolOptions;
        }
    }

    /// <summary>Restores the built-in fallbacks. For tests that need to prove the unconfigured path.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _ssrf = new BuiltInSsrfPolicy();
            _toolOptions = null;
        }
    }

    private sealed class BuiltInSsrfPolicy : ISsrfPolicy
    {
        // Mirrors AnthillRuntime.SsrfBlockedHostnames / SsrfBlockedHostSuffixes as declared.
        public IReadOnlySet<string> BlockedHostnames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };

        public IReadOnlyList<string> BlockedHostSuffixes { get; } = new[] { ".localhost", ".local" };
    }
}
