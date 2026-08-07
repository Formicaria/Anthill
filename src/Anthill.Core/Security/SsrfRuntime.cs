using Anthill.Core.Configuration;
using Anthill.SDK.Security;

namespace Anthill.Core.Security;

/// <summary>
/// The colony's live outbound blocklist, and the default <c>UrlSafety</c> falls back to. v3.8.12.
///
/// The exact counterpart of <see cref="Anthill.Core.Tools.ToolRuntime"/>, for the same reason: it
/// bridges <see cref="AnthillRuntime"/> — mutable statics that the operator surface and the test
/// suite both write — to an SDK interface that cannot reference the core.
///
/// Both properties READ THROUGH on every access. A captured copy would stop tracking a host added
/// after startup, which is the one thing an SSRF blocklist must never do.
/// </summary>
public sealed class SsrfRuntime : ISsrfPolicy
{
    /// <summary>The default installed by <see cref="SafetyPolicyBootstrap"/> when the core loads.</summary>
    public static readonly SsrfRuntime Live = new();

    private SsrfRuntime() { }

    public IReadOnlySet<string> BlockedHostnames => AnthillRuntime.SsrfBlockedHostnames;

    public IReadOnlyList<string> BlockedHostSuffixes => AnthillRuntime.SsrfBlockedHostSuffixes;
}
